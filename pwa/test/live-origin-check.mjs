// A read-only check of the app at its public address (#393): `node test/live-origin-check.mjs [origin]`,
// origin defaulting to https://app.slipgates.com. It drives Chromium at the origin, signs in to nothing
// and pairs nothing, and aborts every request that is not to the origin itself, so the relay is never
// contacted. It is not a spec (no .spec in the name): the Playwright suites run against a local build.
// Exit code 1 if any check fails. Needs `npm ci` in pwa/ and Playwright's Chromium installed. The
// page's own requests are aborted unless they are to the origin, and the script's direct requests
// (`get`) refuse any other address, so nothing can reach the relay or a third party.
import { chromium } from '@playwright/test';

const origin = (process.argv[2] ?? 'https://app.slipgates.com').replace(/\/+$/, '');
const results = [];
const check = async (name, fn) => {
  try {
    const detail = await fn();
    results.push(true);
    console.log(`ok    ${name}${detail ? ` (${detail})` : ''}`);
  } catch (e) {
    results.push(false);
    console.log(`FAIL  ${name} - ${e.message}`);
  }
};
const expect = (cond, message) => { if (!cond) { throw new Error(message); } };

const get = (url) => {
  if (new URL(url).origin !== origin) { throw new Error(`refusing to request ${url}`); }
  return context.request.get(url, { maxRedirects: 0 });
};

const browser = await chromium.launch();
const context = await browser.newContext();
const blocked = [];
await context.route('**/*', (route) => {
  const url = route.request().url();
  if (new URL(url).origin === origin) { return route.continue(); }
  blocked.push(url);
  return route.abort();
});
const page = await context.newPage();

await check('first load at the root is 200 HTML titled Slipgates', async () => {
  const r = await page.goto(`${origin}/`);
  expect(r.status() === 200, `status ${r.status()}`);
  expect(/text\/html/.test(r.headers()['content-type'] ?? ''), `type ${r.headers()['content-type']}`);
  expect((await page.title()) === 'Slipgates', `title ${await page.title()}`);
});

await check('the service worker registers with scope /', async () => {
  const scope = await page.evaluate(async () => (await navigator.serviceWorker.ready).scope);
  expect(scope === `${origin}/`, `scope ${scope}`);
  return scope;
});

await check('the manifest identity, start URL and scope resolve to the origin root', async () => {
  const href = await page.evaluate(() => document.querySelector('link[rel=manifest]').href);
  const r = await get(href);
  expect(/manifest\+json|application\/json/.test(r.headers()['content-type'] ?? ''), `type ${r.headers()['content-type']}`);
  const m = await r.json();
  const at = (v) => new URL(v, href).href;
  expect(at(m.id) === `${origin}/`, `id ${at(m.id)}`);
  expect(at(m.start_url) === `${origin}/`, `start_url ${at(m.start_url)}`);
  expect(at(m.scope) === `${origin}/`, `scope ${at(m.scope)}`);
  for (const icon of m.icons) {
    const i = await get(at(icon.src));
    expect(i.status() === 200 && (i.headers()['content-type'] ?? '').startsWith('image/'), `${icon.src} ${i.status()} ${i.headers()['content-type']}`);
  }
  return `${m.icons.length} icons served`;
});

await check('the app takes control after a reload, so it can serve itself', async () => {
  await page.reload();
  const controlled = await page.evaluate(() => !!navigator.serviceWorker.controller);
  expect(controlled, 'no controller');
});

for (const path of ['/', '/index.html', '/?demo=1', '/?demo=1#/settings', '/#/machines']) {
  await check(`direct navigation and refresh of ${path} load the app`, async () => {
    for (const again of [false, true]) {
      const r = again ? await page.reload() : await page.goto(`${origin}${path}`);
      expect(r === null || r.status() === 200, `status ${r?.status()}`);
      expect((await page.title()) === 'Slipgates', `title ${await page.title()}`);
      expect(await page.locator('.shell').count() === 1, 'no .shell');
    }
  });
}

await check('the Pages 404 is not served as the app shell for an unknown path', async () => {
  const r = await page.goto(`${origin}/sessions`);
  expect(r.status() === 404, `status ${r.status()}`);
  expect((await page.title()) !== 'Slipgates', 'the app answered an unknown path');
});

await check('a trailing slash redirect for /app and /s2 stays on the origin', async () => {
  for (const p of ['/app', '/s2']) {
    const r = await get(`${origin}${p}`);
    expect(r.status() === 301 && new URL(r.headers().location, origin).origin === origin, `${p} ${r.status()} ${r.headers().location}`);
  }
});

await check('/s2/ is not answered with the app shell, and /app/ is the redirect page', async () => {
  await page.goto(`${origin}/`);
  await page.goto(`${origin}/s2/`, { waitUntil: 'domcontentloaded' });
  expect(new URL(page.url()).pathname === '/s2/', `/s2/ ended at ${page.url()}`);
  expect(!(await page.locator('.shell').count()), '/s2/ showed the app shell');
  const moved = await (await get(`${origin}/app/`)).text();
  expect(/has moved/.test(moved) && !/class="shell"/.test(moved), '/app/ is not the moved page');
});

await check('relay-style paths are not intercepted by the worker', async () => {
  await page.goto(`${origin}/`);
  const seen = await page.evaluate(async () => {
    const out = {};
    for (const p of ['/v1/push/key', '/v1/anything', '/health']) {
      const r = await fetch(p, { credentials: 'include', headers: { authorization: 'Bearer not-a-real-token' } });
      out[p] = { status: r.status, type: r.headers.get('content-type') };
    }
    return out;
  });
  for (const [p, r] of Object.entries(seen)) {
    expect(r.status === 404, `${p} gave ${r.status}`);
  }
  return Object.entries(seen).map(([p, r]) => `${p} ${r.status}`).join(', ');
});

await check('no entry for another address, /v1/, /sessions or /health is in the caches', async () => {
  const kept = await page.evaluate(async () => {
    const all = {};
    for (const k of await caches.keys()) { all[k] = (await (await caches.open(k)).keys()).map((r) => r.url); }
    return all;
  });
  const urls = Object.values(kept).flat();
  expect(urls.length > 20, `only ${urls.length} entries cached`);
  const bad = urls.filter((u) => new URL(u).origin !== origin || u.includes('/v1/') || /\/(sessions|health)$/.test(u));
  expect(bad.length === 0, `unexpected: ${bad.join(', ')}`);
  return `${urls.length} entries in ${Object.keys(kept).join(', ')}`;
});

await check('the app reloads offline from the worker', async () => {
  await page.goto(`${origin}/`);
  await context.setOffline(true);
  try {
    await page.reload();
    expect((await page.title()) === 'Slipgates', `title ${await page.title()}`);
    expect(await page.locator('.shell').count() === 1, 'no .shell');
  } finally {
    await context.setOffline(false);
  }
});

// Informational: the app only reaches elsewhere (the relay) once a machine is paired, and every
// such request is aborted above, so a list here never means something was sent.
console.log(`info  requests aborted because they left the origin: ${blocked.length ? [...new Set(blocked)].join(', ') : 'none'}`);

await browser.close();
const failed = results.filter((r) => !r).length;
console.log(failed ? `${failed} check(s) failed.` : 'All checks passed.');
process.exit(failed ? 1 : 0);
