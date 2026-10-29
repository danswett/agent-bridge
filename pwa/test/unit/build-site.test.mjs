import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, readFileSync, readdirSync, rmSync, writeFileSync, appendFileSync } from 'node:fs';
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
  for (const f of ['index.html', 'app.css', 'sw.js', 'manifest.webmanifest', 'icon.svg', 'js', 'fixtures']) {
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

test('the offline shell holds every script the app is made of, so it starts with no network', () => {
  const shell = new Set([...readFileSync(path.join(pwa, 'sw.js'), 'utf8').matchAll(/'\.\/(js\/[^']+)'/g)].map((m) => m[1]));
  const scripts = readdirSync(path.join(pwa, 'js'), { recursive: true })
    .map((f) => `js/${f.split(path.sep).join('/')}`)
    .filter((f) => f.endsWith('.js'));
  assert.deepEqual(scripts.filter((f) => !shell.has(f)), []);
});

test('the page may reach the relay and the one storage account its blob store uses, and nothing else', () => {
  const csp = contentSecurityPolicy('https://relay.example', ['https://abblobprodwus2.blob.core.windows.net']);
  assert.match(csp, /connect-src 'self' https:\/\/relay\.example wss:\/\/relay\.example https:\/\/abblobprodwus2\.blob\.core\.windows\.net;/);
  for (const bad of ['https://evil.example', 'http://acct.blob.core.windows.net', 'https://acct.blob.core.windows.net/path', "https://acct.blob.core.windows.net; script-src *"]) {
    assert.throws(() => contentSecurityPolicy('https://relay.example', [bad]), /--blob-origin must be an origin/);
  }
});
