import { defineConfig, devices } from '@playwright/test';
import base from './playwright.config.mjs';

// Renders of the PWA's demo for design review (pwa/test/renders): captures, not checks. Design
// signs a state off against what the product draws, not a board, and nothing may run on an
// owner's machine, so CI draws them (ci.yml pwa-ui, Chromium) and keeps pwa/renders as an
// artifact. Chromium alone: it is the engine that emulates forced colours.
export default defineConfig({
  ...base,
  testDir: 'test/renders',
  testMatch: /.*\.render\.mjs$/,
  testIgnore: undefined,
  // Its own, not test-results: Playwright empties a run's output folder as it starts, and the
  // tests' folder still holds what a failed run left, which CI uploads after this run.
  outputDir: 'test-results-renders',
  retries: 0,
  reporter: [['list']],
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});