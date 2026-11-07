import { test } from '@playwright/test';
import { THEMES, openDemo, shoot } from './shoot.mjs';

// The dividers between the panes (#1030, ui.md §5.2): the sessions page at the widths the panes
// change at (two panes at 760 and 1024, three at 1440), with the list pane widened and the
// divider it was moved by focused, and the inspector's divider pointed at where there is one.
const SIZES = [760, 1024, 1440].map((w) => ({ name: String(w), viewport: { width: w, height: 800 } }));

for (const width of SIZES) {
  for (const theme of THEMES) {
    test(`the pane dividers, ${theme.name}, ${width.name}`, async ({ page }) => {
      await page.setViewportSize(width.viewport);
      await page.emulateMedia(theme.media);
      await openDemo(page);
      await page.locator('.session').first().click();
      const three = width.viewport.width >= 1280;
      await page.locator('#list-divider').focus();
      for (let i = 0; i < 4; i++) { await page.keyboard.press('ArrowRight'); }
      if (three) {
        const box = await page.locator('#inspector-divider').boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + 300);
      }
      await shoot(page, 'panes', theme, width);
    });
  }
}
