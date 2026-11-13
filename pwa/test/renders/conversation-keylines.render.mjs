import { test } from '@playwright/test';
import { open, motionsDone } from '../nav.mjs';
import { eachLook, openDemo, shoot } from './shoot.mjs';

eachLook(test, 'the conversation keylines', async ({ page, theme, width }) => {
  await openDemo(page, '&delegates=1');
  const view = await open(page, 'demo-delegates');
  await motionsDone(page);
  await shoot(page, 'conversation-keylines', theme, width);
  if (width.viewport.width >= 1280) {
    await view.locator('.inspector-button').click();
    await motionsDone(page);
    await shoot(page, 'conversation-keylines-no-details', theme, width);
  }
});
