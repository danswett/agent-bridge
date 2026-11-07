import { test } from '@playwright/test';
import { go } from '../nav.mjs';
import { eachLook, openDemo, shoot } from './shoot.mjs';

// No held bar (ui.md §6.6, §7; #1064): what stands in its place. The page's clock is the
// capture's, so a hold's two minutes pass at once.
const REASON = 'an unrecognised copilot process (pid 4120)';
const HELD = `Held: session discovery is waiting on ${REASON}, so whether that session is still running cannot be told yet. Try again in a minute or two.`;

eachLook(test, 'the machine row after a discovery hold has lasted two minutes', async ({ page, theme, width }) => {
  await openDemo(page);
  await page.clock.install();
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 1000);
  await page.evaluate((reason) => window.agentBridgeDemo.hold({ reason, agents: ['copilot'] }), REASON);
  await page.clock.runFor(2 * 60000 + 100);
  await go(page, 'machines');
  await page.locator('.machine .machine-flag.held').waitFor();
  await shoot(page, 'held-row', theme, width);
});

// A resume the host holds back while discovery is held: the waiting form, not a refusal.
eachLook(test, 'a resume held back while discovery is held', async ({ page, theme, width }) => {
  await openDemo(page);
  await go(page, 'launch');
  await page.locator('#launch [name="resume"]').selectOption('7d3c1a2e-5b6f-4c8d-9e0a-1b2c3d4e5f60');
  await page.evaluate((reason) => window.agentBridgeDemo.scriptLaunches({ outcome: 'busy', reason }), HELD);
  await page.locator('#launch').getByRole('button', { name: 'Launch' }).click();
  await page.locator('#launch .launch-status', { hasText: 'it resumes once discovery finishes' }).evaluate(async (el) => {
    await new Promise((r) => setTimeout(r, 300));
    for (let p = el.parentElement; p; p = p.parentElement) { p.scrollTop = p.scrollHeight; }
    window.scrollTo(0, document.documentElement.scrollHeight);
  });
  await shoot(page, 'held-resume', theme, width);
});

// A lasting state's banner, opened to its full height.
eachLook(test, 'a lasting banner fully open', async ({ page, theme, width }) => {
  await openDemo(page);
  await page.evaluate(async () => {
    const { reveal } = await import('./js/ui/motion.js');
    reveal(document.getElementById('replaced-banner'));
  });
  await page.locator('#replaced-banner').waitFor({ state: 'visible' });
  await page.evaluate(() => Promise.all(document.getElementById('replaced-banner').getAnimations().map((a) => a.finished.catch(() => {}))));
  await shoot(page, 'banner-open', theme, width);
});
