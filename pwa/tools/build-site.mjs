// Assembles the PWA for publishing: copies the app, writes config.js with the relay it
// talks to, and puts the Content-Security-Policy in the page (GitHub Pages cannot send
// headers). The tests serve exactly this output, so they run under the same policy.
//
//   node tools/build-site.mjs --out <dir> [--relay https://relay.example]
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const FILES = ['index.html', 'app.css', 'sw.js', 'manifest.webmanifest', 'icon.svg', 'js', 'fixtures'];

export function relayOrigin(relay) {
  if (!relay) { return ''; }
  const u = new URL(relay);
  if (!['https:', 'http:'].includes(u.protocol) || u.origin !== relay.replace(/\/$/, '')) {
    throw new Error(`--relay must be an origin like https://relay.example, not ${relay}`);
  }
  return u.origin;
}

export function contentSecurityPolicy(relay) {
  const origin = relayOrigin(relay);
  // 'self' does not cover WebSockets in every Safari, so the socket origin is named too.
  const connect = origin ? ` ${origin} ${origin.replace(/^http/, 'ws')}` : '';
  // Scripts and styles only from this site, network only to the relay: a page that holds
  // the device key must not be able to load or send anything else.
  return `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'${connect}; object-src 'none'; base-uri 'none'; form-action 'none'`;
}

export function buildSite({ out, relay = '' }) {
  const origin = relayOrigin(relay);
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  for (const f of FILES) { cpSync(path.join(root, f), path.join(out, f), { recursive: true }); }
  writeFileSync(path.join(out, 'config.js'), `export const RELAY = ${JSON.stringify(origin)};\n`);
  const index = path.join(out, 'index.html');
  const html = readFileSync(index, 'utf8');
  const marker = '<meta charset="utf-8">';
  if (!html.includes(marker)) { throw new Error('index.html has no <meta charset="utf-8"> to put the policy after'); }
  const csp = contentSecurityPolicy(origin).replace(/"/g, '&quot;');
  writeFileSync(index, html.replace(marker, `${marker}\n  <meta http-equiv="Content-Security-Policy" content="${csp}">`));
  return out;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const arg = (name) => { const k = process.argv.indexOf(name); return k > 0 ? process.argv[k + 1] : undefined; };
  const out = arg('--out');
  if (!out) { console.error('usage: build-site.mjs --out <dir> [--relay <origin>]'); process.exit(2); }
  buildSite({ out: path.resolve(out), relay: arg('--relay') ?? '' });
  console.log(`built ${out}${arg('--relay') ? ` for ${arg('--relay')}` : ''}`);
}
