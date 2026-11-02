// Generates every client's copy of the design language from design/ (design/README.md):
//
//   node tools/design-gen/generate.mjs           writes the generated files
//   node tools/design-gen/generate.mjs --check   writes nothing; fails naming each stale file
//
// Outputs: pwa/design/tokens.css and pwa/design/sprite.svg; the PWA's theme colours in
// pwa/index.html and pwa/manifest.webmanifest; src/AgentBridge.App/Design/DesignResources.xaml
// and DesignKeys.cs; and the layout sizes for the app's view models in
// src/AgentBridge.App.Core/DesignLayout.cs. design/preview.png is drawn by preview.mjs, which
// needs a browser.

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { loadTokens, surfaceTokens, monoFont } from './lib/tokens.mjs';
import { readDesignDir } from './lib/svg.mjs';
import { tokensCss, spriteSvg } from './lib/emit-pwa.mjs';
import { designXaml, designKeysCs, designLayoutCs } from './lib/emit-xaml.mjs';
import { toCssColor } from './lib/color.mjs';

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

// The PWA's browser chrome follows the shell: the status bar on a phone, and the splash screen.
function themeColours(root, tokens) {
  const shell = surfaceTokens(tokens).find((s) => s.name === 'shell');
  const indexFile = path.join(root, 'pwa', 'index.html');
  let index = readFileSync(indexFile, 'utf8');
  for (const scheme of ['light', 'dark']) {
    const re = new RegExp(`(<meta name="theme-color" content=")[^"]*(" media="\\(prefers-color-scheme: ${scheme}\\)">)`);
    if (!re.test(index)) { throw new Error(`pwa/index.html has no theme-color meta for ${scheme}`); }
    index = index.replace(re, `$1${toCssColor(shell[scheme])}$2`);
  }
  const manifestFile = path.join(root, 'pwa', 'manifest.webmanifest');
  let manifest = readFileSync(manifestFile, 'utf8');
  for (const key of ['background_color', 'theme_color']) {
    const re = new RegExp(`("${key}": ")[^"]*(")`);
    if (!re.test(manifest)) { throw new Error(`pwa/manifest.webmanifest has no ${key}`); }
    // One value only: the dark shell, which the installed app has always opened on.
    manifest = manifest.replace(re, `$1${toCssColor(shell.dark)}$2`);
  }
  return { 'pwa/index.html': index, 'pwa/manifest.webmanifest': manifest };
}

// Every face the clients are told about must exist, as WOFF2 for the PWA and TTF for the app,
// with its licence: a face that isn't there would fail quietly in the PWA and leave the app
// pointing at a file it doesn't have.
function checkFontFiles(root, tokens) {
  const font = monoFont(tokens);
  if (!font) { return; }
  const dir = path.join(root, font.dir);
  const wanted = [...font.faces.flatMap((f) => [`woff2/${f.file}.woff2`, `ttf/${f.file}.ttf`]), font.license];
  const missing = wanted.filter((f) => !existsSync(path.join(dir, f)));
  if (missing.length) { throw new Error(`type.mono.files (${font.dir}) lacks ${missing.join(', ')}`); }
}

export function generate(root = repoRoot) {
  const design = path.join(root, 'design');
  const tokens = loadTokens(design);
  checkFontFiles(root, tokens);
  const glyphs = readDesignDir(path.join(design, 'glyphs'), 'glyph');
  const icons = readDesignDir(path.join(design, 'icons'), 'icon');
  return {
    'pwa/design/tokens.css': tokensCss(tokens),
    'pwa/design/sprite.svg': spriteSvg(glyphs, icons),
    ...themeColours(root, tokens),
    'src/AgentBridge.App/Design/DesignResources.xaml': designXaml(tokens, glyphs, icons),
    'src/AgentBridge.App/Design/DesignKeys.cs': designKeysCs(tokens, glyphs, icons),
    'src/AgentBridge.App.Core/DesignLayout.cs': designLayoutCs(tokens),
  };
}

// Line endings aside: a Windows checkout may hold any file with CRLF.
const same = (a, b) => a.replace(/\r\n/g, '\n') === b.replace(/\r\n/g, '\n');

export function staleFiles(root = repoRoot) {
  return Object.entries(generate(root))
    .filter(([rel, content]) => { const f = path.join(root, rel); return !existsSync(f) || !same(readFileSync(f, 'utf8'), content); })
    .map(([rel]) => rel);
}

export function write(root = repoRoot) {
  const written = [];
  for (const [rel, content] of Object.entries(generate(root))) {
    const f = path.join(root, rel);
    if (existsSync(f) && same(readFileSync(f, 'utf8'), content)) { continue; }
    mkdirSync(path.dirname(f), { recursive: true });
    writeFileSync(f, content);
    written.push(rel);
  }
  return written;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  if (process.argv.includes('--check')) {
    const stale = staleFiles();
    if (stale.length) {
      console.error(`Stale against design/ (run node tools/design-gen/generate.mjs and commit the result):\n${stale.map((s) => `  ${s}`).join('\n')}`);
      process.exit(1);
    }
    console.log('Every generated file matches design/.');
  } else {
    const written = write();
    console.log(written.length ? `Wrote:\n${written.map((s) => `  ${s}`).join('\n')}` : 'Every generated file already matches design/.');
  }
}
