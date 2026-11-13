import { test, expect } from '@playwright/test';
import { motionsDone, row, selectSort, view } from './nav.mjs';

const ID = 'demo-status-artifact';

for (const phone of [false, true]) {
  test.describe(phone ? 'touch phone' : 'desktop', () => {
    test.use({
      viewport: { width: phone ? 390 : 1440, height: 900 },
      hasTouch: phone,
      isMobile: phone,
    });

    for (const state of ['idle', 'working', 'pinned']) {
      test(`${state}: returning to the list leaves only the status glyph in its icon area (#1641)`, async ({ page }, info) => {
        await page.goto('?demo=1&static=1');
        await page.waitForSelector('html[data-demo-ready]');
        const status = state === 'idle' ? 'idle' : 'working';
        await page.evaluate(({ id, status }) => {
          const now = new Date().toISOString();
          window.agentBridgeDemo.upsert({
            id, title: 'Status artifact check', agent: 'copilot', status,
            startedAt: now, updatedAt: now, statusSince: now, activity: {},
          });
        }, { id: ID, status });
        const item = row(page, ID);
        const button = item.locator('.row-main');
        const icon = item.locator('.row-glyph');
        if (state === 'pinned') {
          await button.press('Shift+F10');
          await item.getByRole('menuitem', { name: 'Pin', exact: true }).click();
          await expect(item).toHaveAttribute('data-pinned', 'true');
        }
        await motionsDone(page);
        const before = info.outputPath('icon-before.png');
        await icon.screenshot({ path: before });
        await info.attach('icon-before', { path: before, contentType: 'image/png' });
        if (phone) { await button.tap(); } else { await button.click(); }
        await expect(view(page, ID)).toBeVisible();
        if (phone) {
          await view(page, ID).locator('.session-bar .back').tap();
        } else {
          await page.locator('#nav [data-nav="sessions"]').click();
        }
        await expect(item).toBeVisible();
        await motionsDone(page);
        // A pointer returning to its row reveals the grip; touch must leave it hidden.
        if (!phone) { await button.hover(); }
        await expect(icon.locator('.glyph')).toHaveAttribute('data-glyph', status);
        const after = info.outputPath('icon-after.png');
        await icon.screenshot({ path: after });
        await info.attach('icon-after', { path: after, contentType: 'image/png' });
        const drawn = await icon.evaluate((el) => {
          const box = el.getBoundingClientRect();
          const overlaps = (other) => {
            const r = other.getBoundingClientRect();
            return Math.max(0, Math.min(box.right, r.right) - Math.max(box.left, r.left)) *
              Math.max(0, Math.min(box.bottom, r.bottom) - Math.max(box.top, r.top));
          };
          const painted = (other) => {
            const css = getComputedStyle(other);
            return css.display !== 'none' && css.visibility !== 'hidden' && Number(css.opacity) > 0;
          };
          const grip = el.closest('.session').querySelector('.drag-handle');
          const copies = [...document.body.children].filter((c) =>
            c.matches('div[aria-hidden="true"]') && c.inert && getComputedStyle(c).position === 'fixed');
          return {
            grip: { background: getComputedStyle(grip).backgroundImage, overlap: painted(grip) ? overlaps(grip) : 0 },
            copies: copies.filter((c) => painted(c) && overlaps(c) > 0).length,
            glyphs: el.querySelectorAll('.glyph').length,
          };
        });
        console.log(`${info.project.name}, ${phone ? 'touch' : 'desktop'}, ${state}: ${JSON.stringify(drawn)}`);
        await info.attach('icon-dom', { body: JSON.stringify(drawn, null, 2), contentType: 'application/json' });
        expect(drawn.copies, 'no exit copy overlays the status icon').toBe(0);
        expect(drawn.glyphs).toBe(1);
        expect(drawn.grip.overlap, `the dotted Move grip (${drawn.grip.background}) must not cover the status icon`).toBe(0);
        if (phone) { await expect(item.locator('.drag-handle')).toHaveCSS('display', 'none'); }
      });
    }

    test('the internal Move gutter keeps card edges aligned and its glyph still at rest, hover or keyed focus', async ({ page }, info) => {
      await page.goto('?demo=1&static=1');
      await page.waitForSelector('html[data-demo-ready]');
      const item = row(page, '019a1b2c-codex-example');
      const button = item.locator('.row-main');
      const grip = item.locator('.drag-handle');
      const inks = await page.evaluate(() => {
        const probe = document.createElement('i');
        document.body.append(probe);
        const colors = ['--ab-text3', '--ab-text'].map((token) => {
          probe.style.color = `var(${token})`;
          return getComputedStyle(probe).color;
        });
        probe.remove();
        return colors;
      });
      await button.press('Shift+F10');
      await item.getByRole('menuitem', { name: 'Pin', exact: true }).click();
      const measure = () => item.evaluate((el) => {
        const rowBox = el.getBoundingClientRect();
        const glyph = el.querySelector('.row-glyph').getBoundingClientRect();
        const handle = el.querySelector('.drag-handle');
        const box = handle.getBoundingClientRect();
        return {
          rowX: rowBox.left,
          headingX: document.querySelector('.list-group').getBoundingClientRect().left,
          glyphX: glyph.left - rowBox.left,
          overlap: Math.max(0, Math.min(glyph.right, box.right) - Math.max(glyph.left, box.left)) *
            Math.max(0, Math.min(glyph.bottom, box.bottom) - Math.max(glyph.top, box.top)),
        };
      });
      await selectSort(page, 'recent');
      await motionsDone(page);
      const mainOffset = (await measure()).glyphX;
      // Most recent keeps today's inset; only Your order reserves the new grip column.
      expect(mainOffset).toBe(12);
      for (const sort of ['recent', 'yours']) {
        await selectSort(page, sort);
        await motionsDone(page);
        await page.mouse.move(0, 0);
        for (const input of ['rest', 'hover', 'key']) {
          if (input === 'hover' && !phone) { await button.hover(); }
          if (input === 'key') {
            await button.focus();
            await page.keyboard.press('Shift+Tab');
            await page.keyboard.press('Tab');
            await expect(button).toBeFocused();
          }
          const box = await measure();
          expect(box.rowX, `${sort}, ${input}: card edge aligns with its heading`).toBe(box.headingX);
          expect(box.glyphX, `${sort}, ${input}: glyph retains its row-relative inset`).toBe(mainOffset + (sort === 'yours' ? 24 : 0));
          expect(box.overlap, `${sort}, ${input}: grip box stays outside the glyph box`).toBe(0);
          if (sort === 'recent' || (phone && input !== 'key')) {
            await expect(grip).toHaveCSS('display', 'none');
          } else {
            await expect(grip).toBeVisible();
            await expect(grip).toHaveCSS('opacity', '1');
            await expect(grip).toHaveCSS('color', inks[input === 'rest' ? 0 : 1]);
          }
        }
      }
      if (!phone) {
        await page.getByRole('separator', { name: 'Resize the session list', exact: true }).press('Home');
        expect((await page.locator('#list-pane').boundingBox()).width).toBe(264);
        await button.hover();
        const narrow = await measure();
        expect(narrow.glyphX).toBe(mainOffset + 24);
        expect(narrow.overlap).toBe(0);
        expect(narrow.rowX).toBe(narrow.headingX);
        const screenshot = info.outputPath('list-264.png');
        await page.locator('#list-pane').screenshot({ path: screenshot });
        await info.attach('list-264', { path: screenshot, contentType: 'image/png' });
      }
      await page.evaluate(() => {
        const demo = window.agentBridgeDemo;
        for (const [id, title] of [
          ['019a1b2c-codex-example', 'Gutter match one'],
          ['6f1c2b7e-claude-example', 'Gutter match two'],
        ]) { demo.upsert({ ...demo.host.get(id), title }); }
      });
      await page.keyboard.press('Control+k');
      const search = page.getByRole('searchbox', { name: 'Search sessions' });
      for (const [query, count, inset] of [['Gutter match one', 1, 0], ['Gutter match', 2, 24]]) {
        await search.fill(query);
        await expect(page.locator('.session')).toHaveCount(count);
        const box = await measure();
        expect(box.glyphX, `${count} filtered rows reserve the appropriate column`).toBe(mainOffset + inset);
        expect(box.rowX).toBe(box.headingX);
        expect(box.overlap).toBe(0);
        // Filtering still disables reordering; reserving its column never enables a move.
        await expect(grip).toHaveCSS('display', 'none');
      }
    });
  });
}
