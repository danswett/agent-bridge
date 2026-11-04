import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync, appendFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite, contentSecurityPolicy } from '../../tools/build-site.mjs';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const scratch = path.join(pwa, '.site', 'unit-build');
const out = (name) => path.join(scratch, name);
const sw = (dir) => readFileSync(path.join(dir, 'sw.js'), 'utf8');

// A copy of the app to change, so the real one is never touched.
function appCopy(name) {
  const root = out(name);
  for (const f of ['index.html', 'app.css', 'design', 'sw.js', 'manifest.webmanifest', 'icon.svg', 'icons', 'js', 'fixtures']) {
    cpSync(path.join(pwa, f), path.join(root, f), { recursive: true });
  }
  return root;
}

test('the service worker changes whenever anything the site serves changes', () => {
  rmSync(scratch, { recursive: true, force: true });
  const root = appCopy('src');
  const a = buildSite({ root, out: out('a'), relay: 'https://relay.example' });
  const same = buildSite({ root, out: out('same'), relay: 'https://relay.example' });
  assert.equal(sw(a), sw(same), 'the same input builds the same worker');

  const relay = buildSite({ root, out: out('relay'), relay: 'https://other-relay.example' });
  assert.notEqual(sw(relay), sw(a), 'a new relay is a new version');

  appendFileSync(path.join(root, 'app.css'), '\n.x { color: red; }\n');
  const css = buildSite({ root, out: out('css'), relay: 'https://relay.example' });
  assert.notEqual(sw(css), sw(a), 'a stylesheet change is a new version');

  writeFileSync(path.join(root, 'js', 'demo.js'), `${readFileSync(path.join(root, 'js', 'demo.js'), 'utf8')}\n// changed\n`);
  const js = buildSite({ root, out: out('js'), relay: 'https://relay.example' });
  assert.notEqual(sw(js), sw(css), 'a script change is a new version');
  rmSync(scratch, { recursive: true, force: true });
});

test('the page loads the app as one minified bundle without the demo, preloads what it imports, and keeps all of it offline', () => {
  const site = buildSite({ out: out('bundle') });
  const html = readFileSync(path.join(site, 'index.html'), 'utf8');
  const scripts = [...html.matchAll(/<script type="module" src="([^"]+)"/g)].map((m) => m[1]);
  assert.equal(scripts.length, 1);
  assert.match(scripts[0], /^bundle\/main-\w+\.js$/, 'the entry, under a name that carries its content\'s hash');
  const files = readdirSync(path.join(site, 'bundle')).map((f) => `bundle/${f}`);
  const shell = new Set([...sw(site).matchAll(/'\.\/([^']+)'/g)].map((m) => m[1]));
  const demo = files.filter((f) => /^bundle\/demo-\w+\.js$/.test(f));
  assert.equal(demo.length, 1, 'the demo is a chunk of its own, fetched only when the demo starts');
  assert.ok(!shell.has(demo[0]), 'and never precached');
  assert.deepEqual(files.filter((f) => !demo.includes(f) && !shell.has(f)), [], 'everything else the app may ask for works offline');
  assert.deepEqual([...shell].filter((f) => f.startsWith('js/')), [], 'the source layout is served, but nothing loads it');
  const preloaded = [...html.matchAll(/<link rel="modulepreload" href="([^"]+)">/g)].map((m) => m[1]);
  assert.deepEqual(preloaded.filter((f) => !shell.has(f) || demo.includes(f)), []);
  const size = (dir, list) => list.reduce((n, f) => n + statSync(path.join(dir, f)).size, 0);
  const sources = readdirSync(path.join(site, 'js'), { recursive: true }).filter((f) => f.endsWith('.js'));
  const bundled = size(site, files.filter((f) => !demo.includes(f)));
  assert.ok(bundled < size(path.join(site, 'js'), sources) / 2, `the bundle (${bundled} bytes) is minified`);
  rmSync(scratch, { recursive: true, force: true });
});
test('a shell file the site lacks fails the build, so no release goes out that no app could install', () => {
  rmSync(scratch, { recursive: true, force: true });
  const root = appCopy('lacking');
  const worker = path.join(root, 'sw.js');
  writeFileSync(worker, readFileSync(worker, 'utf8').replace("'./app.css',", "'./app.css', './gone.css',"));
  assert.throws(() => buildSite({ root, out: out('lacking-site') }), /sw\.js keeps files the site lacks.*gone\.css/);
  rmSync(scratch, { recursive: true, force: true });
});

test('the page may reach the relay and the one storage account its blob store uses, and nothing else', () => {
  const csp = contentSecurityPolicy('https://relay.example', ['https://abblobprodwus2.blob.core.windows.net']);
  assert.match(csp, /connect-src 'self' https:\/\/relay\.example wss:\/\/relay\.example https:\/\/abblobprodwus2\.blob\.core\.windows\.net;/);
  for (const bad of ['https://evil.example', 'http://acct.blob.core.windows.net', 'https://acct.blob.core.windows.net/path', "https://acct.blob.core.windows.net; script-src *"]) {
    assert.throws(() => contentSecurityPolicy('https://relay.example', [bad]), /--blob-origin must be an origin/);
  }
});

test('the built site carries both families\' WOFF2s and each one\'s OFL licence, and keeps every shell file offline', () => {
  const site = buildSite({ out: out('fonts') });
  const fonts = readdirSync(path.join(site, 'design', 'fonts')).sort();
  // Slipgates Sans' variable pair, and Slipgates Mono's four weights, each with an italic.
  assert.equal(fonts.filter((f) => f.endsWith('.woff2')).length, 10);
  assert.deepEqual(fonts.filter((f) => f.endsWith('.txt')), ['SlipgatesMono-OFL.txt', 'SlipgatesSans-OFL.txt'], 'each licence travels with its fonts');
  const shell = [...sw(site).matchAll(/'\.\/([^']*)'/g)].map((m) => m[1]).filter(Boolean);
  // A shell file the site lacks fails the worker's install, so the app never works offline.
  assert.deepEqual(shell.filter((f) => !existsSync(path.join(site, f))), []);
  assert.deepEqual(fonts.filter((f) => f.endsWith('.woff2') && !shell.includes(`design/fonts/${f}`)), []);
  // Every face tokens.css asks for is there.
  const css = readFileSync(path.join(site, 'design', 'tokens.css'), 'utf8');
  const faces = [...css.matchAll(/url\("(fonts\/[^"]+)"\)/g)].map((m) => m[1]);
  assert.equal(faces.length, 10);
  assert.deepEqual(faces.filter((f) => !existsSync(path.join(site, 'design', f))), []);
  rmSync(scratch, { recursive: true, force: true });
});
