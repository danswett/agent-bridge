import { defineConfig, devices } from '@playwright/test';

// The PWA in Chromium and WebKit (Safari's engine), served as GitHub Pages serves it:
// built by tools/build-site.mjs, under /agent-bridge/app/, with the CSP in the page.
// test/e2e/ also needs the spike dev stack built (see test/e2e/e2e.spec.mjs).
// PWA_TEST_PORT moves it, for a machine running another checkout's tests at the same time.
const PORT = Number(process.env.PWA_TEST_PORT) || 8790;

export default defineConfig({
  testDir: 'test',
  testMatch: /.*\.spec\.mjs$/,
  timeout: 60000,
  fullyParallel: false,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [['list'], ['github']] : [['list']],
  use: {
    baseURL: `http://127.0.0.1:${PORT}/agent-bridge/app/`,
    // So page.route sees every request; the worker gets its own test with workers allowed.
    serviceWorkers: 'block',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
  ],
  webServer: {
    command: `node test/serve.mjs --port ${PORT}`,
    url: `http://127.0.0.1:${PORT}/agent-bridge/app/`,
    reuseExistingServer: !process.env.CI,
  },
});
