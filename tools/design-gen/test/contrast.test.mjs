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
  for (const fg of ['surface.text', 'surface.text2', 'surface.text3', 'needs.text', 'needs.head', 'agent.text', 'bridge', 'danger']) {
    assert.ok(covered(fg, 'text'), `${fg} is measured as text on every surface`);
  }
  assert.ok(pairs.some((p) => p.fg === 'needs.text' && p.bg.startsWith('needs.fill')), 'needs text is measured on the needs fill');
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

