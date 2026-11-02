import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { repoRoot } from '../generate.mjs';

const read = (rel) => readFileSync(path.join(repoRoot, rel), 'utf8');
const withoutComments = (css) => css.replace(/\/\*[\s\S]*?\*\//g, '');

// CSS's named colours, short of transparent and currentColor, which carry no colour of their own.
const NAMED = /(?<![\w-])(white|black|red|green|blue|yellow|orange|purple|gray|grey|silver|maroon|navy|teal|olive|lime|aqua|fuchsia|pink|gold|crimson|tomato)(?![\w-])/i;

test('the PWA\'s stylesheet sets no colour of its own: every colour is a generated token', () => {
  const css = withoutComments(read('pwa/app.css'));
  const found = [
    ...[...css.matchAll(/#[0-9a-f]{3,8}\b/gi)].map((m) => m[0]),
    ...[...css.matchAll(/\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\([^)]*\)/gi)].map((m) => m[0]),
    ...css.split('\n').filter((l) => /(?:^|[;{\s])(?:color|background(?:-color)?|border(?:-\w+)?-color|fill|stroke|outline-color|box-shadow|accent-color)\s*:/.test(l) && NAMED.test(l)),
  ];
  assert.deepEqual(found, []);
});

test('every custom property the PWA\'s stylesheet uses is defined, by it or by the generated tokens', () => {
  // A var() naming a token that was renamed or never existed is no colour at all, silently.
  const css = withoutComments(read('pwa/app.css'));
  const defined = new Set([...`${css}\n${read('pwa/design/tokens.css')}`.matchAll(/(--[\w-]+)\s*:/g)].map((m) => m[1]));
  const used = new Set([...css.matchAll(/var\((--[\w-]+)/g)].map((m) => m[1]));
  assert.deepEqual([...used].filter((v) => !defined.has(v)), []);
  assert.ok(used.has('--ab-bridge') && used.has('--ab-shell'), 'it does use the tokens');
});

test('the PWA loads the generated tokens before its own stylesheet, and keeps them offline', () => {
  const html = read('pwa/index.html');
  const tokens = html.indexOf('href="design/tokens.css"');
  assert.ok(tokens > 0 && tokens < html.indexOf('href="app.css"'));
  assert.match(read('pwa/sw.js'), /'\.\/design\/tokens\.css', '\.\/design\/sprite\.svg'/);
});

test('no colour copied by hand while the generator was missing is left in the PWA', () => {
  // U1 (#164) marked its stand-ins TODO(#177); the generated tokens replace them.
  const files = ['pwa/app.css', 'pwa/index.html', ...readdirSync(path.join(repoRoot, 'pwa/js'), { recursive: true })
    .map((f) => `pwa/js/${String(f).split(path.sep).join('/')}`).filter((f) => f.endsWith('.js'))];
  assert.deepEqual(files.filter((f) => read(f).includes('TODO(#177)')), []);
});
