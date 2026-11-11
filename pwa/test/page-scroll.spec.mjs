import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';
const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const frame = (page) => page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
for (const width of [390, 1024]) {
  test.describe(`installed at ${width} px`, () => {
    // Mobile WebKit cannot send wheel input; keep touch and the installed-device layout.
    test.use({ viewport: { width, height: 844 }, hasTouch: true, isMobile: false,
      userAgent: 'Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1' });

    test('scrolling beyond the conversation bottom leaves the page and header in place', async ({ page }) => {
      await page.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }));
      await page.goto('?demo=1&static=1');
      await page.waitForSelector('html[data-demo-ready]');
      await page.evaluate((id) => {
        const d = window.agentBridgeDemo;
        const s = d.host.get(id);
        d.upsert({ ...s, question: null, activity: { ...s.activity, reasoning: null,
          response: Array.from({ length: 80 }, (_, i) => `Paragraph ${i}. A long conversation.`).join('\n\n') } });
      }, IDLE);
      const view = await open(page, IDLE);
      const scroll = view.locator('.stream-scroll');
      expect.soft(await scroll.evaluate((e) => getComputedStyle(e).overflowY)).toBe('auto');
      expect.soft(await page.evaluate(() => getComputedStyle(document.documentElement).overflowY)).toBe('hidden');
      if (await page.evaluate(() => CSS.supports('overscroll-behavior-y', 'contain'))) {
        expect.soft(await scroll.evaluate((e) => getComputedStyle(e).overscrollBehaviorY)).toBe('contain');
        expect.soft(await page.evaluate(() => getComputedStyle(document.documentElement).overscrollBehaviorY)).toBe('none');
      }
      // Synthetic outer overflow exposes chaining; desktop emulation has no iOS rubber band.
      await page.evaluate(() => {
        const spacer = document.createElement('div');
        spacer.style.height = '100vh'; document.body.append(spacer);
      });
      await expect.poll(() => scroll.evaluate((e) => e.scrollHeight - e.clientHeight)).toBeGreaterThan(100);
      await scroll.evaluate((e) => { e.scrollTop = e.scrollHeight; });
      await frame(page);
      const header = width < 760 ? view.locator('.session-top') : page.locator('#appbar');
      const before = await header.boundingBox();
      const box = await scroll.boundingBox();
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      for (let i = 0; i < 3; i++) {
        await page.mouse.wheel(0, 1000);
        await frame(page);
      }
      expect(await page.evaluate(() => scrollY)).toBe(0);
      expect(await header.boundingBox()).toEqual(before);
      expect(await page.evaluate(() => document.documentElement.scrollHeight - innerHeight)).toBeLessThanOrEqual(0);
      await expect(header).toBeInViewport();
    });
    test('the body stays locked without the root :has enhancement', async ({ page }) => {
      await page.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }));
      await page.route(/app\.css$/, async (route) => {
        const response = await route.fetch();
        // An unknown pseudo-class makes the parser reject the entire enhancement rule.
        await route.fulfill({ response, body: (await response.text()).replace(':root:has(', ':root:unsupported-has(') });
      });
      await page.goto('?demo=1&static=1');
      await page.waitForSelector('html[data-demo-ready]');
      await open(page, IDLE);
      expect(await page.evaluate(() => getComputedStyle(document.body).overflowY)).toBe('hidden');
      expect(await page.evaluate(() => document.body.getBoundingClientRect().height)).toBe(844);
    });
  });
}
