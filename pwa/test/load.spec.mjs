// How long a phone waits for the first session (#609), cold and with the service worker in
// control, measured the way the footprint audit measured it: a phone's screen, a CPU 4× slower,
// and 150 ms a request at 1.6 Mbps over HTTP/1.1 (the test server's). Chromium only, as only its
// DevTools can throttle. The times go to the job's summary; the test fails only on what can't
// vary with the runner: a warm start that asks the network for any of the release's files.
import { test, expect } from '@playwright/test';
import { appendFileSync } from 'node:fs';

test.skip(({ browserName }) => browserName !== 'chromium', 'only Chromium can throttle the network and the CPU');
test.use({ serviceWorkers: 'allow', viewport: { width: 412, height: 915 }, isMobile: true, hasTouch: true });

const KBPS = 1600;

async function throttle(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false,
    latency: 150,
    downloadThroughput: (KBPS * 1000) / 8,
    uploadThroughput: (750 * 1000) / 8,
    connectionType: 'cellular4g',
  });
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
}

// When the first session row went into the page, in ms since the navigation began.
async function firstRow(page) {
  await page.waitForFunction(() => window.__firstRow !== undefined, null, { timeout: 30000 });
  return Math.round(await page.evaluate(() => window.__firstRow));
}

test('a warm start shows the first session from the release\'s cache, asking the network for none of its files', async ({ page, context }) => {
  await context.addInitScript(() => {
    new MutationObserver((_, o) => {
      if (document.querySelector('.session')) { window.__firstRow = performance.now(); o.disconnect(); }
    }).observe(document, { childList: true, subtree: true });
  });
  const network = [];
  page.on('requestfinished', async (r) => {
    const res = await r.response();
    network.push({ url: r.url(), fromWorker: res?.fromServiceWorker() ?? false, bytes: (await r.sizes().catch(() => null))?.responseBodySize ?? 0 });
  });
  await throttle(page);

  await page.goto('?demo=1&static=1');
  const cold = await firstRow(page);
  const coldRequests = network.length;
  const coldBytes = network.reduce((n, r) => n + r.bytes, 0);
  // The worker installs its release beside the first load and takes the page over.
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null, null, { timeout: 30000 });
  await page.evaluate(async () => { await navigator.serviceWorker.ready; });

  // What the worker asks the network for, from here on.
  const worker = context.serviceWorkers().find((w) => w.url().endsWith('/sw.js')) ?? await context.waitForEvent('serviceworker');
  await worker.evaluate(() => {
    self.__fetched = [];
    const real = self.fetch.bind(self);
    self.fetch = (input, init) => { self.__fetched.push(new URL(input.url ?? input, self.registration.scope).pathname); return real(input, init); };
  });

  network.length = 0;
  await page.reload();
  const warm = await firstRow(page);
  const fromNetwork = [...network.filter((r) => !r.fromWorker).map((r) => new URL(r.url).pathname), ...await worker.evaluate(() => self.__fetched)];

  const lines = [
    '### PWA load (#609): mobile viewport, CPU 4×, 150 ms a request at 1.6 Mbps',
    '',
    '| Load | First session row | Requests |',
    '|---|---:|---:|',
    `| Cold | ${cold} ms | ${coldRequests} (${Math.round(coldBytes / 1024)} KB) |`,
    `| Warm (service worker) | ${warm} ms | ${network.length}, ${fromNetwork.length} from the network |`,
    '',
  ];
  console.log(lines.join('\n'));
  test.info().annotations.push({ type: 'cold first row', description: `${cold} ms` }, { type: 'warm first row', description: `${warm} ms` });
  if (process.env.GITHUB_STEP_SUMMARY) { appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${lines.join('\n')}\n`); }

  // The demo's chunk is no file of the release's (only the demo asks for it); it was kept the
  // first time, and is refreshed behind the page.
  expect(fromNetwork.filter((p) => !/\/bundle\/demo-\w+\.js$/.test(p))).toEqual([]);
});
