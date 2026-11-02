// Assembles the PWA for publishing: copies the app, writes config.js with the relay it
// talks to, and puts the Content-Security-Policy in the page (GitHub Pages cannot send
// headers). The tests serve exactly this output, so they run under the same policy.
//
//   node tools/build-site.mjs --out <dir> [--relay https://relay.example] [--blob-origin https://<account>.blob.core.windows.net]
import { cpSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const appRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
// design/: the generated tokens.css and sprite.svg (tools/design-gen); copyFonts adds the fonts.
const FILES = ['index.html', 'app.css', 'design', 'sw.js', 'manifest.webmanifest', 'icon.svg', 'js', 'fixtures'];

export function relayOrigin(relay) {
  if (!relay) { return ''; }
  const u = new URL(relay);
  if (!['https:', 'http:'].includes(u.protocol) || u.origin !== relay.replace(/\/$/, '')) {
    throw new Error(`--relay must be an origin like https://relay.example, not ${relay}`);
  }
  return u.origin;
}

// The relay's blob store, where large attachments are uploaded straight from the page: an Azure
// Storage account's blob endpoint, and nothing else.
export function blobOrigin(url) {
  let u = null;
  try { u = new URL(url); } catch { /* refused below */ }
  if (!u || u.protocol !== 'https:' || !/^[a-z0-9]{3,24}\.blob\.core\.windows\.net$/.test(u.hostname) || u.origin !== url.replace(/\/$/, '')) {
    throw new Error(`--blob-origin must be an origin like https://account.blob.core.windows.net, not ${url}`);
  }
  return u.origin;
}

export function contentSecurityPolicy(relay, blobOrigins = []) {
  const origin = relayOrigin(relay);
  // 'self' does not cover WebSockets in every Safari, so the socket origin is named too.
  const connect = (origin ? ` ${origin} ${origin.replace(/^http/, 'ws')}` : '') + blobOrigins.map((b) => ` ${blobOrigin(b)}`).join('');
  // Scripts and styles only from this site, network only to the relay and its blob store: a
  // page that holds the device key must not be able to load or send anything else. blob:
  // images are the thumbnails of files about to be attached, made in the page from their own
  // bytes.
  return `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'${connect}; object-src 'none'; base-uri 'none'; form-action 'none'`;
}

// The monospace's WOFF2s and its licence, from the repository's design/ (design/tokens.json
// type.mono.files) into design/fonts/, where design/tokens.css's @font-face rules look. Copied
// at build time rather than kept twice in the repository; the OFL travels with them.
export function copyFonts(out, repo = path.dirname(appRoot)) {
  const mono = JSON.parse(readFileSync(path.join(repo, 'design', 'tokens.json'), 'utf8')).type.mono;
  if (!mono.files) { return []; }
  const from = path.join(repo, mono.files);
  const to = path.join(out, 'design', 'fonts');
  mkdirSync(to, { recursive: true });
  const files = [...readdirSync(path.join(from, 'woff2')).filter((f) => f.endsWith('.woff2')).map((f) => path.join('woff2', f)), 'OFL.txt'];
  for (const f of files) { cpSync(path.join(from, f), path.join(to, path.basename(f))); }
  return files.map((f) => path.basename(f));
}

export function buildSite({ out, relay = '', blobOrigins = [], root = appRoot }) {
  const origin = relayOrigin(relay);
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  for (const f of FILES) { cpSync(path.join(root, f), path.join(out, f), { recursive: true }); }
  copyFonts(out);
  writeFileSync(path.join(out, 'config.js'), `export const RELAY = ${JSON.stringify(origin)};\n`);
  const index = path.join(out, 'index.html');
  const html = readFileSync(index, 'utf8');
  const marker = '<meta charset="utf-8">';
  if (!html.includes(marker)) { throw new Error('index.html has no <meta charset="utf-8"> to put the policy after'); }
  const csp = contentSecurityPolicy(origin, blobOrigins).replace(/"/g, '&quot;');
  writeFileSync(index, html.replace(marker, `${marker}\n  <meta http-equiv="Content-Security-Policy" content="${csp}">`));
  stampWorker(out);
  return out;
}

// An installed PWA that stays open runs its old code until a new service worker takes
// over, and a browser installs one only when sw.js itself changes. So sw.js carries a
// hash of every other file served, and any change to the app or its relay is an update.
function stampWorker(out) {
  const files = [];
  const walk = (dir) => {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { walk(p); } else { files.push(p); }
    }
  };
  walk(out);
  const sw = path.join(out, 'sw.js');
  const hash = createHash('sha256');
  for (const f of files.filter((f) => f !== sw).sort()) {
    hash.update(path.relative(out, f).split(path.sep).join('/')).update('\0').update(readFileSync(f)).update('\0');
  }
  const placeholder = "const VERSION = 'dev';";
  const source = readFileSync(sw, 'utf8');
  if (!source.includes(placeholder)) { throw new Error(`sw.js has no ${placeholder} to stamp`); }
  writeFileSync(sw, source.replace(placeholder, `const VERSION = '${hash.digest('hex').slice(0, 16)}';`));
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const arg = (name) => { const k = process.argv.indexOf(name); return k > 0 ? process.argv[k + 1] : undefined; };
  const out = arg('--out');
  if (!out) { console.error('usage: build-site.mjs --out <dir> [--relay <origin>] [--blob-origin <origin>]'); process.exit(2); }
  buildSite({ out: path.resolve(out), relay: arg('--relay') ?? '', blobOrigins: arg('--blob-origin') ? [arg('--blob-origin')] : [] });
  console.log(`built ${out}${arg('--relay') ? ` for ${arg('--relay')}` : ''}`);
}
