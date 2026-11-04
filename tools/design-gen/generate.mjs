// Generates every client's copy of the design language from design/ (design/README.md):
//
//   node tools/design-gen/generate.mjs           writes the generated files
//   node tools/design-gen/generate.mjs --check   writes nothing; fails naming each stale file
//
// Outputs: pwa/design/tokens.css and pwa/design/sprite.svg; the PWA's theme colours in
// pwa/index.html and pwa/manifest.webmanifest; src/AgentBridge.App/Design/DesignResources.xaml
// and DesignKeys.cs; and the layout sizes for the app's view models in
// src/AgentBridge.App.Core/DesignLayout.cs. The bundled fonts for the Apple and Android apps:
// design/apple/SlipgatesFonts.swift and .plist, and design/android/font/ (lib/emit-native.mjs).
// With them, the app icon (lib/app-icon.mjs):
// design/app-icon/*.svg, pwa/icon.svg, pwa/icons/*.png, and the app's Assets/AgentBridge.ico
// and AgentBridge.png, whose rasters are compared by their pixels. design/preview.png is drawn
// by preview.mjs, which needs a browser.

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { loadTokens, surfaceTokens, bundledFonts, checkNoSynthesis, motion, MOTION_PLATFORMS } from './lib/tokens.mjs';
import { readDesignDir } from './lib/svg.mjs';
import { tokensCss, spriteSvg } from './lib/emit-pwa.mjs';
import { designXaml, designKeysCs, designLayoutCs } from './lib/emit-xaml.mjs';
import { appleFontsSwift, appleFontsPlist, androidFonts } from './lib/emit-native.mjs';
import { toCssColor } from './lib/color.mjs';
import { staleAppIcons, writeAppIcons } from './lib/app-icon.mjs';

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

// Every face the clients are told about must exist, with its licence: a face that isn't there
// would fail quietly in the PWA and leave an app pointing at a file it doesn't have. And each
// family's faces.json (written by its build) must hold exactly the real faces the tokens
// promise, in the collection's order, so no client is told of a weight or style it would have
// to fake (docs/design/ui.md §4.2). Returns each family's faces.json, by kind.
function checkFontFiles(root, tokens) {
  checkNoSynthesis(tokens);
  const manifests = {};
  for (const font of bundledFonts(tokens)) {
    const dir = path.join(root, font.dir);
    const wanted = [
      font.license, 'faces.json', font.collection,
      ...(font.variable
        ? font.variableFiles.flatMap((v) => [`ttf/${v.file}.ttf`, `woff2/${v.file}.woff2`])
        : font.faces.flatMap((f) => [`woff2/${f.file}.woff2`, `ttf/${f.file}.ttf`])),
    ];
    const missing = wanted.filter((f) => !existsSync(path.join(dir, f)));
    if (missing.length) { throw new Error(`type.${font.kind}.files (${font.dir}) lacks ${missing.join(', ')}`); }
    const manifest = JSON.parse(readFileSync(path.join(dir, 'faces.json'), 'utf8'));
    const where = `${font.dir}/faces.json`;
    if (manifest.family !== font.family) { throw new Error(`${where} is "${manifest.family}", not ${font.family}`); }
    const listed = manifest.faces.map((f) => `${f.weight} ${f.style}`);
    const promised = font.faces.map((f) => `${f.weight} ${f.style}`);
    if (listed.join() !== promised.join()) { throw new Error(`${where} has faces ${listed.join(', ')}; type.${font.kind} promises ${promised.join(', ')}`); }
    if (manifest.faces.some((f, i) => f.index !== i)) { throw new Error(`${where}: a face's index is not its place in ${font.collection}`); }
    const ttc = readFileSync(path.join(dir, font.collection));
    if (ttc.toString('latin1', 0, 4) !== 'ttcf' || ttc.readUInt32BE(8) !== font.faces.length) { throw new Error(`${font.dir}/${font.collection} is not a collection of ${font.faces.length} faces`); }
    manifests[font.kind] = manifest;
  }
  return manifests;
}

export function generate(root = repoRoot) {
  const design = path.join(root, 'design');
  const tokens = loadTokens(design);
  const manifests = checkFontFiles(root, tokens);
  // Every platform's motion, not only the two generated today: tokens for the Apple and Android
  // apps are held to ui.md §5.3 from the start, not when their generator arrives.
  for (const p of MOTION_PLATFORMS) { motion(tokens, p); }
  const glyphs = readDesignDir(path.join(design, 'glyphs'), 'glyph');
  const icons = readDesignDir(path.join(design, 'icons'), 'icon');
  return {
    'pwa/design/tokens.css': tokensCss(tokens),
    'pwa/design/sprite.svg': spriteSvg(glyphs, icons),
    ...themeColours(root, tokens),
    'src/AgentBridge.App/Design/DesignResources.xaml': designXaml(tokens, glyphs, icons),
    'src/AgentBridge.App/Design/DesignKeys.cs': designKeysCs(tokens, glyphs, icons, manifests),
    'src/AgentBridge.App.Core/DesignLayout.cs': designLayoutCs(tokens),
    'design/apple/SlipgatesFonts.swift': appleFontsSwift(tokens, manifests),
    'design/apple/SlipgatesFonts.plist': appleFontsPlist(tokens, manifests),
    ...androidFonts(root, tokens),
  };
}

// Line endings aside: a Windows checkout may hold any file with CRLF. A font file is compared
// byte for byte.
const same = (file, content) => (Buffer.isBuffer(content)
  ? readFileSync(file).equals(content)
  : readFileSync(file, 'utf8').replace(/\r\n/g, '\n') === content.replace(/\r\n/g, '\n'));

export function staleFiles(root = repoRoot) {
  return Object.entries(generate(root))
    .filter(([rel, content]) => { const f = path.join(root, rel); return !existsSync(f) || !same(f, content); })
    .map(([rel]) => rel);
}

export function write(root = repoRoot) {
  const written = [];
  for (const [rel, content] of Object.entries(generate(root))) {
    const f = path.join(root, rel);
    if (existsSync(f) && same(f, content)) { continue; }
    mkdirSync(path.dirname(f), { recursive: true });
    writeFileSync(f, content);
    written.push(rel);
  }
  return written;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  if (process.argv.includes('--check')) {
    // The app icon's rasters take seconds to draw, so they stay out of generate(), which the tests
    // call many times over, and have their own test.
    const stale = [...staleFiles(), ...staleAppIcons(repoRoot)];
    if (stale.length) {
      console.error(`Stale against design/ (run node tools/design-gen/generate.mjs and commit the result):\n${stale.map((s) => `  ${s}`).join('\n')}`);
      process.exit(1);
    }
    console.log('Every generated file matches design/.');
  } else {
    const written = [...write(), ...writeAppIcons(repoRoot)];
    console.log(written.length ? `Wrote:\n${written.map((s) => `  ${s}`).join('\n')}` : 'Every generated file already matches design/.');
  }
}
