import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// S12 owns unit tests only; browser specs are a separate machine-name slice.
const root = fileURLToPath(new URL('./', import.meta.url));

test('unit test fixtures and comments use neutral machine names', () => {
  const violations = [];
  for (const file of readdirSync(root, { recursive: true }).filter((file) => file.endsWith('.mjs')).sort()) {
    const lines = readFileSync(path.join(root, file), 'utf8').split(/\r?\n/);
    lines.forEach((line, index) => {
      if (/VM[1-9]|DSWETT[-]/.test(line)) violations.push(`${file}:${index + 1}`);
    });
  }
  assert.deepEqual(violations, [], 'use neutral fixture names at these unit-test locations');
});
