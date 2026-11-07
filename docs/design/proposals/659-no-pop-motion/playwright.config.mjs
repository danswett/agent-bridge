import { defineConfig, devices } from '../../../../pwa/node_modules/@playwright/test/index.mjs';

// The #659 prototype's tests, in Chromium and WebKit. Not part of the PWA's suite: run from pwa/
// (whose node_modules has Playwright) with
//   npx playwright test --config ../docs/design/proposals/659-no-pop-motion/playwright.config.mjs
export default defineConfig({
  testDir: '.',
  testMatch: /prototype\.spec\.mjs$/,
  outputDir: '../../../../pwa/test-results/proposal-659',
  timeout: 30000,
  reporter: [['list']],
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
  ],
});
