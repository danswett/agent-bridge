// The product's name has one source in the PWA, js/brand.js (#538), so the rename (#535) is a
// change there and a run of tools/brand.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { BRAND } from '../../js/brand.js';
import { appRoot, brandFiles, staleBrandFiles } from '../../tools/brand.mjs';
import { deviceName } from '../../js/identity.js';

const read = (f) => readFileSync(path.join(appRoot, f), 'utf8');
const RENAMED = { name: 'Renamed & "Co" <App>', shortName: "Rename's" };

test('the page, the manifest and the service worker name the product as js/brand.js does', () => {
  assert.deepEqual(staleBrandFiles(), [], 'run node tools/brand.mjs');
  const manifest = JSON.parse(read('manifest.webmanifest'));
  assert.equal(manifest.name, BRAND.name);
  assert.equal(manifest.short_name, BRAND.shortName);
  const page = read('index.html');
  assert.equal(/<title>([^<]*)<\/title>/.exec(page)[1], BRAND.name);
  assert.equal(/<meta name="apple-mobile-web-app-title" content="([^"]*)">/.exec(page)[1], BRAND.shortName);
});

test('a new name in js/brand.js reaches every place the page, the manifest and the worker show the old one', () => {
  const renamed = brandFiles(appRoot, RENAMED);
  for (const [rel, text] of Object.entries(renamed)) {
    assert.ok(!text.includes(BRAND.name), `${rel} still says ${BRAND.name}`);
  }
  const manifest = JSON.parse(renamed['manifest.webmanifest']);
  assert.deepEqual([manifest.name, manifest.short_name], [RENAMED.name, RENAMED.shortName]);
  const page = renamed['index.html'];
  assert.match(page, /<title>Renamed &amp; &quot;Co&quot; &lt;App&gt;<\/title>/);
  assert.match(page, /aria-label="Renamed &amp; &quot;Co&quot; &lt;App&gt;: sessions"/);
  assert.equal(page.match(/<span(?: class="[^"]*")? data-brand>Renamed &amp; &quot;Co&quot; &lt;App&gt;<\/span>/g).length, 3,
    'the wordmark, sign-in and the empty machines list');
  // The worker, run with the new name, titles a push that has none with it.
  let shown = null;
  const self = { registration: { scope: 'https://example.test/app/', showNotification: async (title) => { shown = title; } }, addEventListener: (type, fn) => { if (type === 'push') { self.push = fn; } }, skipWaiting() {}, clients: {} };
  vm.runInNewContext(renamed['sw.js'], { self, location: new URL(self.registration.scope), URL, caches: {}, fetch });
  self.push({ data: null, waitUntil() {} });
  assert.equal(shown, RENAMED.name);
});

test('a short name longer than a home screen shows is refused', () => {
  assert.throws(() => brandFiles(appRoot, { name: 'A long product name', shortName: 'A long product name' }), /longer than the 12 characters/);
});

test('no script names the product but js/brand.js, so none keeps the old name after a rename', () => {
  const scripts = readdirSync(path.join(appRoot, 'js'), { recursive: true })
    .map((f) => `js/${f.split(path.sep).join('/')}`)
    .filter((f) => f.endsWith('.js') && f !== 'js/brand.js');
  assert.deepEqual(scripts.filter((f) => read(f).includes(BRAND.name)), []);
});

