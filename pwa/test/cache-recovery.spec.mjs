import { test, expect } from '@playwright/test';
import { writeFile, unlink } from 'node:fs/promises';
import path from 'node:path';

// Worker termination is available through Chromium's DevTools protocol only.
test.use({ serviceWorkers: 'allow' });
test.skip(({ browserName }) => browserName !== 'chromium', 'worker termination requires Chromium DevTools');

test('an updated asset survives worker termination and an offline reopen', async ({ page, context }) => {
  const name = 'cache-recovery-test.txt';
  const file = path.resolve('.site/demo', name);
  await writeFile(file, 'before');
  try {
    await page.goto('?demo=1&static=1');
    await page.evaluate(async () => { await navigator.serviceWorker.ready; });
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller);
    const read = () => page.evaluate(async (asset) => (await fetch(asset)).text(), name);
    expect(await read()).toBe('before');
    await writeFile(file, 'after');
    expect(await read()).toBe('after');
    await expect.poll(() => page.evaluate(async (asset) => (await caches.match(asset))?.text(), name)).toBe('after');
    const cdp = await context.newCDPSession(page);
    await cdp.send('ServiceWorker.enable');
    await cdp.send('ServiceWorker.stopAllWorkers');
    await context.setOffline(true);
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller);
    expect(await read()).toBe('after');
  } finally {
    await context.setOffline(false);
    await unlink(file);
  }
});
