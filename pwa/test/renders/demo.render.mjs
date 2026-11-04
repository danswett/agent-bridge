import { test } from '@playwright/test';
import { eachLook, openDemo, shoot } from './shoot.mjs';

// The demo's sessions list in each theme and width: the renders' own baseline, so the route is
// seen to work before any feature adds its states.
eachLook(test, 'the sessions list', async ({ page, theme, width }) => {
  await openDemo(page);
  await shoot(page, 'sessions', theme, width);
});