import { defineConfig, devices } from '@playwright/test';

// The PWA in Chromium and WebKit (Safari's engine), served as GitHub Pages serves it:
// built by tools/build-site.mjs, at the site's root, with the CSP in the page.
// test/e2e/ also needs the spike dev stack built (see test/e2e/e2e.spec.mjs).
// PWA_TEST_PORT moves it, for a machine running another checkout's tests at the same time.
const PORT = Number(process.env.PWA_TEST_PORT) || 8790;

export default defineConfig({
  testDir: 'test',
  testMatch: /.*\.spec\.mjs$/,
  // CI runs test/e2e in a job of its own, the one that builds the dev stack those specs need
  // (ci.yml pwa-e2e). Its other Playwright jobs set PWA_SKIP_E2E to leave them out, rather than
  // have them skip themselves there.
  testIgnore: process.env.PWA_SKIP_E2E ? '**/e2e/**' : undefined,
  timeout: 60000,
  fullyParallel: false,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [['list'], ['github']] : [['list']],
  // One picture per engine for every platform (test/glyphs.spec.mjs): CI draws on Linux, and a
  // glyph's snapshot is about its shape and colours, which a tolerance holds across platforms.
  snapshotPathTemplate: '{testDir}/{testFileDir}/{testFileName}-snapshots/{arg}-{projectName}{ext}',
  use: {
    baseURL: `http://127.0.0.1:${PORT}/`,
    // So page.route sees every request; the worker gets its own test with workers allowed.
    serviceWorkers: 'block',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
  ],
  webServer: {
    command: `node test/serve.mjs --port ${PORT}`,
    url: `http://127.0.0.1:${PORT}/`,
    reuseExistingServer: !process.env.CI,
  },
});
