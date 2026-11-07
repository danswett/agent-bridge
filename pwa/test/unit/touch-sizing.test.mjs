import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../../app.css', import.meta.url), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');

// An iPad keeps (pointer: coarse) with a trackpad attached, and no browser here can emulate that,
// so the rule is held here: touch sizes only where touch is the only pointer (ui.md §9.1 A11).
test('every touch-sized rule in app.css also requires that no fine pointer is attached', () => {
  const queries = [...css.matchAll(/@media([^{]*)\{/g)].map((m) => m[1].toLowerCase().replace(/\s+/g, ''));
  const touchy = /pointer:coarse|hover:none/;
  const parts = queries.flatMap((q) => q.split(',')).filter((p) => touchy.test(p));
  assert.ok(parts.length > 0);
  for (const part of parts) {
    assert.match(part, /^(\([a-z-]+:[^()]+\)and)*\(pointer:coarse\)and\(not\(any-pointer:fine\)\)$/, `@media ${part}`);
  }
});
