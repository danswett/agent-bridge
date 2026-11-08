import { defineConfig, devices } from '../../../../pwa/node_modules/@playwright/test/index.mjs';

// The #1193 fix's captures: the PWA's demo, drawn into this folder. Not part of the PWA's suite: run
// from pwa/ (whose node_modules has Playwright) with
//   npx playwright test --config ../docs/design/proposals/1193-stale-lines-fix/playwright.config.mjs
const PORT = Number(process.env.PWA_TEST_PORT) || 8794;

export default defineConfig({
  testDir: '.',
  testMatch: /capture\.spec\.mjs$/,
  outputDir: '../../../../pwa/test-results/proposal-1193-fix',
  timeout: 90000,
  workers: 1,
  reporter: [['list']],
  use: { baseURL: `http://127.0.0.1:${PORT}/`, serviceWorkers: 'block' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `node test/serve.mjs --port ${PORT}`,
    cwd: '../../../../pwa',
    url: `http://127.0.0.1:${PORT}/`,
    reuseExistingServer: false,
  },
});
