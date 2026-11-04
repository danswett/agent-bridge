// Writes the product's name from js/brand.js into the files that can't import it: the page's
// title, home-screen title and labels, the manifest's name and short name, and the service
// worker's title for a notification that has none. Scripts read js/brand.js themselves.
//
//   node tools/brand.mjs           writes them
//   node tools/brand.mjs --check   writes nothing; fails naming each stale file
//
// test/unit/brand.test.mjs runs the check, so a hand edit of the name in one of them fails.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { BRAND } from '../js/brand.js';

export const appRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));

// What a home screen shows under the icon before it cuts the label short.
export const SHORT_NAME_MAX = 12;

const html = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const json = (s) => JSON.stringify(s);
const js = (s) => `'${s.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;

// Every place each file shows the name: a pattern around it, which of the names goes there, and
// how it is written. Each pattern must be found, so a reworded page fails here rather than
// keeping the old name. In the page, a <span data-brand> marks the name inside a sentence.
const SLOTS = {
  'index.html': [
    [/(<title>)[^<]*(<\/title>)/g, 'name', html],
    [/(<meta name="apple-mobile-web-app-title" content=")[^"]*(">)/g, 'shortName', html],
    [/(<a class="brand" href="#\/sessions" aria-label=")[^"]*?(: sessions">)/g, 'name', html],
    [/(<nav id="nav" class="nav" aria-label=")[^"]*(")/g, 'name', html],
    [/(<span(?: class="[^"]*")? data-brand>)[^<]*(<\/span>)/g, 'name', html],
  ],
  'manifest.webmanifest': [
    [/("name": )"(?:[^"\\]|\\.)*"()/g, 'name', json],
    [/("short_name": )"(?:[^"\\]|\\.)*"()/g, 'shortName', json],
  ],
  'sw.js': [
    [/(\nconst NAME = )'(?:[^'\\]|\\.)*'(;)/g, 'name', js],
  ],
};

export function checkBrand(brand) {
  for (const key of ['name', 'shortName']) {
    if (typeof brand[key] !== 'string' || !brand[key].trim()) { throw new Error(`BRAND.${key} must be a name`); }
  }
  if ([...brand.shortName].length > SHORT_NAME_MAX) {
    throw new Error(`BRAND.shortName "${brand.shortName}" is longer than the ${SHORT_NAME_MAX} characters a home screen shows`);
  }
}

// Each file as it should be with `brand`'s names in it.
export function brandFiles(root = appRoot, brand = BRAND) {
  checkBrand(brand);
  const out = {};
  for (const [rel, slots] of Object.entries(SLOTS)) {
    let text = readFileSync(path.join(root, rel), 'utf8');
    for (const [re, key, encode] of slots) {
      let found = 0;
      text = text.replace(re, (_, before, after) => { found++; return `${before}${encode(brand[key])}${after}`; });
      if (!found) { throw new Error(`${rel} has no place for the name matching ${re}`); }
    }
    out[rel] = text;
  }
  return out;
}

// Line endings aside: a Windows checkout may hold any file with CRLF.
const same = (a, b) => a.replace(/\r\n/g, '\n') === b.replace(/\r\n/g, '\n');

export function staleBrandFiles(root = appRoot) {
  return Object.entries(brandFiles(root))
    .filter(([rel, text]) => { const f = path.join(root, rel); return !existsSync(f) || !same(readFileSync(f, 'utf8'), text); })
    .map(([rel]) => rel);
}

export function writeBrandFiles(root = appRoot) {
  const written = [];
  for (const [rel, text] of Object.entries(brandFiles(root))) {
    const f = path.join(root, rel);
    if (same(readFileSync(f, 'utf8'), text)) { continue; }
    writeFileSync(f, text);
    written.push(rel);
  }
  return written;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  if (process.argv.includes('--check')) {
    const stale = staleBrandFiles();
    if (stale.length) {
      console.error(`Stale against js/brand.js (run node tools/brand.mjs and commit the result):\n${stale.map((s) => `  ${s}`).join('\n')}`);
      process.exit(1);
    }
    console.log('Every file names the product as js/brand.js does.');
  } else {
    const written = writeBrandFiles();
    console.log(written.length ? `Wrote:\n${written.map((s) => `  ${s}`).join('\n')}` : 'Every file already names the product as js/brand.js does.');
  }
}
