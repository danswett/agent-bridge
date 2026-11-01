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

// --- the Windows app ---

const appDir = 'src/AgentBridge.App';
const appFiles = () => readdirSync(path.join(repoRoot, appDir), { recursive: true })
  .map((f) => String(f).split(path.sep).join('/'))
  .filter((f) => /\.(xaml|cs)$/.test(f) && !f.startsWith('Design/') && !/^(bin|obj)\//.test(f));

test('the Windows app sets no colour of its own: App.xaml and every view take the generated brushes', () => {
  const found = [];
  for (const f of appFiles()) {
    const text = read(`${appDir}/${f}`).replace(/<!--[\s\S]*?-->/g, '').replace(/^\s*\/\/.*$/gm, '');
    for (const m of text.matchAll(/#[0-9a-f]{6}(?:[0-9a-f]{2})?\b|\bColors\.\w+|Color\.FromArgb|ColorHelper\.FromArgb/gi)) { found.push(`${f}: ${m[0]}`); }
  }
  assert.deepEqual(found, []);
  assert.match(read(`${appDir}/App.xaml`), /<ResourceDictionary Source="ms-appx:\/\/\/Design\/DesignResources\.xaml" \/>/);
});

test('every Ab resource the Windows app asks for is defined, in all three themes for a brush', () => {
  // A missing key is no build error: the installed app fails at startup or the moment a view
  // asks for it, with nothing but 0xC000027B.
  const generated = read(`${appDir}/Design/DesignResources.xaml`);
  const themes = [...generated.matchAll(/<ResourceDictionary x:Key="(\w+)">([\s\S]*?)<\/ResourceDictionary>/g)];
  assert.deepEqual(themes.map((t) => t[1]), ['Light', 'Dark', 'HighContrast']);
  const inEvery = (key) => themes.every((t) => t[2].includes(`x:Key="${key}"`));
  const defined = new Set([...`${generated}\n${read(`${appDir}/App.xaml`)}`.matchAll(/x:Key="(Ab\w+)"/g)].map((m) => m[1]));
  const asked = new Set();
  for (const f of appFiles()) {
    const text = read(`${appDir}/${f}`);
    for (const m of text.matchAll(/\{(?:Theme|Static)Resource (Ab\w+)\}|Brush\("(Ab\w+)"\)|Resources\["(Ab\w+)"\]/g)) { asked.add(m[1] ?? m[2] ?? m[3]); }
  }
  assert.ok(asked.has('AbCard') && asked.has('AbFontMono'), 'it does use the generated resources');
  assert.deepEqual([...asked].filter((k) => !defined.has(k)), []);
  for (const m of read(`${appDir}/Ui.cs`).matchAll(/Brush\("(Ab\w+)"\)/g)) { assert.ok(inEvery(m[1]), `${m[1]} is in every theme`); }
});
