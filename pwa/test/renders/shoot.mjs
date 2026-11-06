import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Where the renders go, as <state>-<theme>-<width>.png: pwa/renders, which CI keeps as the
// pwa-renders artifact.
export const OUT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'renders');

// Each theme Design reviews a state in (ui.md §9): dark, light, and forced colours as Chromium
// emulates Windows' contrast themes.
export const THEMES = [
  { name: 'dark', media: { colorScheme: 'dark', forcedColors: 'none' } },
  { name: 'light', media: { colorScheme: 'light', forcedColors: 'none' } },
  { name: 'forced', media: { colorScheme: 'dark', forcedColors: 'active' } },
];

// A phone, and a wide desktop window.
export const WIDTHS = [
  { name: '390', viewport: { width: 390, height: 844 } },
  { name: '1440', viewport: { width: 1440, height: 900 } },
];

export async function openDemo(page, query = '') {
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

// What the page shows now, or only target's box. Motion is stopped so a frame never catches a
// fade half done; a ring is drawn where it stands.
export async function shoot(page, state, theme, width, target = null) {
  mkdirSync(OUT, { recursive: true });
  await (target ?? page).screenshot({ path: path.join(OUT, `${state}-${theme.name}-${width.name}.png`), animations: 'disabled', caret: 'hide' });
}

// Every theme at every width, each a test of its own: one state that can't be reached leaves the
// others drawn.
export function eachLook(test, title, body) {
  for (const width of WIDTHS) {
    for (const theme of THEMES) {
      test(`${title}, ${theme.name}, ${width.name}`, async ({ page }) => {
        await page.setViewportSize(width.viewport);
        await page.emulateMedia(theme.media);
        await body({ page, theme, width });
      });
    }
  }
}