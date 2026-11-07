import { test } from '@playwright/test';
import { THEMES, WIDTHS, eachLook, openDemo, shoot } from './shoot.mjs';

// Coming back to the app (#950, ui.md "Coming back to the app"): the demo's sessions list as the
// app is shown again with its second machine and its own link going away, at the points the rules
// name. The page's clock is the capture's, so each frame is the instant it says.
async function comeBack(page, ms, { linkDown = false, cue = false } = {}) {
  await openDemo(page, '&machines=2');
  // The cue is for a list not whole: the laptop back, but its snapshot not yet.
  if (cue) { await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].holdSnapshot()); }
  await page.clock.install();
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 2000);
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo-laptop', false));
  if (cue) {
    await page.clock.runFor(100);
    await page.evaluate(() => window.agentBridgeDemo.setOnline('demo-laptop', true));
    ms -= 100;
  }
  if (linkDown) { await page.evaluate(() => { window.agentBridgeDemo.connection.ready = false; window.agentBridgeDemo.page.refresh(); }); }
  await page.clock.runFor(ms);
}

// Under 1 s: everything as last seen. At 2 s: the cue in the title row's slot. At 5 s: the away
// states in place, and the strip in the title row.
eachLook(test, 'a return under 1 s, as last seen', async ({ page, theme, width }) => {
  await comeBack(page, 500);
  await shoot(page, 'return-0-5s', theme, width);
});

eachLook(test, 'a return at 2 s, with the cue', async ({ page, theme, width }) => {
  await comeBack(page, 2000, { cue: true });
  await shoot(page, 'return-2s', theme, width);
});

eachLook(test, 'a return at 5 s, with the away states and the strip', async ({ page, theme, width }) => {
  await comeBack(page, 5000, { linkDown: true });
  await shoot(page, 'return-5s', theme, width);
});

test('a return at 5 s under reduced motion', async ({ page }) => {
  const width = WIDTHS[0];
  await page.setViewportSize(width.viewport);
  await page.emulateMedia({ ...THEMES[1].media, reducedMotion: 'reduce' });
  await comeBack(page, 5000, { linkDown: true });
  await shoot(page, 'return-5s-reduced', THEMES[1], width);
});
