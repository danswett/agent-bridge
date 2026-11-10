import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const PREFIX = '0123456789abcdef0123456789abcdef';
const WINDOWS = `@C:\\Users\\demo\\AppData\\Local\\AgentBridge\\default\\attachments\\${PREFIX}-`;
const UNIX = `@/home/demo/.agent-bridge/default/attachments/${PREFIX}-`;

async function message(page, text) {
  await page.evaluate(({ id, text }) => {
    const demo = window.agentBridgeDemo;
    const session = demo.host.get(id);
    demo.upsert({ ...session, updatedAt: new Date().toISOString(), activity: { ...session.activity, userMessage: text } });
  }, { id: IDLE, text });
}

for (const width of [390, 820]) {
  for (const mode of ['light', 'dark', 'contrast']) {
    test.describe(`${width}px ${mode}`, () => {
      test.use({ viewport: { width, height: 900 }, hasTouch: true, colorScheme: mode === 'dark' ? 'dark' : 'light', forcedColors: mode === 'contrast' ? 'active' : 'none' });

      test('sent attachment references show named chips, never raw paths or thumbnails', async ({ page }) => {
        await page.addInitScript(() => {
          window.__cspViolations = [];
          document.addEventListener('securitypolicyviolation', (event) => window.__cspViolations.push(event.violatedDirective));
        });
        await page.goto('?demo=1&static=1');
        await page.waitForSelector('html[data-demo-ready]');
        await message(page, `${WINDOWS}report.pdf ${UNIX}diagram.png What is this?\nKeep this line.`);
        const view = await open(page, IDLE);
        const bubble = view.locator('.user-message .bubble');
        await expect(bubble).toBeVisible();
        await expect(bubble).not.toContainText('\\attachments\\');
        await expect(bubble).not.toContainText('/attachments/');
        await expect(bubble).not.toContainText(PREFIX);
        const chips = bubble.getByRole('list', { name: 'Attachments' }).getByRole('listitem');
        await expect(chips).toHaveText(['report.pdf', 'diagram.png']);
        await expect(bubble.locator('.plain')).toHaveText('What is this?\nKeep this line.');
        expect(await bubble.evaluate((element) => element.firstElementChild.getAttribute('aria-label'))).toBe('Attachments');
        await expect(bubble.locator('img, a, button')).toHaveCount(0);

        await message(page, `${WINDOWS}pasted.png`);
        await expect(bubble).not.toContainText('\\attachments\\');
        await expect(chips).toHaveText(['Pasted image']);
        await expect(bubble.locator('.plain')).toBeHidden();

        const ordinary = 'Keep @C:\\work\\notes.md and text <b>unchanged</b>.';
        await message(page, ordinary);
        await expect(bubble.locator('.plain')).toHaveText(ordinary);
        await expect(chips).toHaveCount(0);
        await expect(bubble.locator('b')).toHaveCount(0);
        expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
      });
    });
  }
}
